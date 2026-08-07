#include "runtime/plume/general/plume_backend.h"

#include "runtime/refii_renderer/plume/general/plume_frame_database.h"
#include "runtime/refii_renderer/plume/general/plume_debug_labels.h"

#include "renderer_runtime.h"

#include "renderdoc_debugger.h"
#include "runtime/cvars.h"
#include "runtime/hook_observer.h"
#include "runtime/refii_renderer/plume/fable/direct/fable_direct_renderer.h"
#include "runtime/refii_renderer/plume/fable/resource/fable_guest_resource_registry.h"
#include "runtime/refii_renderer/plume/fable/direct/fable_direct_state.h"
#include "runtime/plume/general/plume_api_bridge.h"
#include "runtime/refii_renderer/plume/general/plume_renderdoc_capture.h"
#include "runtime/plume/general/plume_diagnostics.h"
#include "runtime/plume/general/plume_imgui_overlay.h"
#include "runtime/refii_renderer/hooks/root/fable_hook_symbols.h"
#include "runtime/shader_library.h"

#include <algorithm>
#include <array>
#include <atomic>
#include <cctype>
#include <chrono>
#include <cstdio>
#include <cstring>
#include <filesystem>
#include <fstream>
#include <functional>
#include <memory>
#include <mutex>
#include <string>
#include <string_view>
#include <unordered_map>
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
  const bool staged_draw_work =
      std::any_of(snapshot.staged_draw_source_counts.begin(),
                  snapshot.staged_draw_source_counts.end(),
                  [](uint32_t count) { return count != 0; });
  return snapshot.submitted_render_command_count != 0 ||
         snapshot.draw_event_count != 0 ||
         snapshot.direct_event_count != 0 ||
         snapshot.draw_count != 0 ||
         snapshot.indexed_draw_count != 0 ||
         snapshot.staged_clear_command_count != 0 ||
         snapshot.staged_resolve_command_count != 0 ||
         staged_draw_work;
}

bool QueuedCommandsContainRenderDocDrawTarget(const std::string& target) {
  if (target.empty()) {
    return false;
  }

  constexpr size_t kMaxRenderDocTargetCommandProbe = 4096;
  return FableDirectQueuedRenderDocTargetPresent(
      target, kMaxRenderDocTargetCommandProbe);
}

bool IsRenderDocRootCoverageCaptureTarget(std::string_view target) {
  return target == "allroots" || target == "rootcoverage" ||
         target == "hookroots";
}

std::string NormalizeRenderDocTargetToken(std::string_view target) {
  std::string normalized;
  normalized.reserve(target.size());
  for (const unsigned char ch : target) {
    if (std::isalnum(ch) != 0) {
      normalized.push_back(static_cast<char>(std::tolower(ch)));
    }
  }
  return normalized;
}


bool ParseRenderDocHookTargetAddress(std::string_view token,
                                     uint32_t& address) {
  if (token.empty()) {
    return false;
  }
  if (token.size() > 2u && token[0] == '0' &&
      (token[1] == 'x' || token[1] == 'X')) {
    token.remove_prefix(2u);
  }
  if (token.empty() || token.size() > 8u) {
    return false;
  }

  uint32_t parsed = 0;
  for (const unsigned char ch : token) {
    const int value =
        ch >= '0' && ch <= '9'
            ? ch - '0'
            : (ch >= 'a' && ch <= 'f'
                   ? ch - 'a' + 10
                   : (ch >= 'A' && ch <= 'F' ? ch - 'A' + 10 : -1));
    if (value < 0) {
      return false;
    }
    parsed = (parsed << 4u) | static_cast<uint32_t>(value);
  }
  address = parsed;
  return true;
}

bool TryGetRenderDocHookRootCaptureToken(std::string_view target,
                                         std::string_view& token) {
  constexpr std::string_view root_prefix = "root:";
  constexpr std::string_view hook_prefix = "hook:";
  if (target.substr(0, root_prefix.size()) == root_prefix) {
    token = target.substr(root_prefix.size());
    return !token.empty();
  }
  if (target.substr(0, hook_prefix.size()) == hook_prefix) {
    token = target.substr(hook_prefix.size());
    return !token.empty();
  }
  return false;
}

bool RenderDocHookRootTargetActive(std::string_view token) {
  const std::string normalized_token = NormalizeRenderDocTargetToken(token);
  if (normalized_token.empty()) {
    return false;
  }

  uint32_t requested_address = 0;
  const bool has_requested_address =
      ParseRenderDocHookTargetAddress(token, requested_address);
  const RendererHookStatsSnapshots hook_stats = GetRendererHookStatsSnapshots();
  for (const RendererHookStatsSnapshot& stats : hook_stats) {
    if (stats.hit_count == 0 && stats.guest_passthrough_count == 0) {
      continue;
    }
    if (has_requested_address && stats.fable_address == requested_address) {
      return true;
    }
    if (NormalizeRenderDocTargetToken(stats.name) == normalized_token) {
      return true;
    }
  }
  return false;
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
    TracyPlotConfig("Refii.DrawPackets",
                    tracy::PlotFormatType::Number,
                    true,
                    false,
                    0x10B981);
    TracyPlotConfig("Refii.CommandPackets",
                    tracy::PlotFormatType::Number,
                    true,
                    false,
                    0x38BDF8);
    TracyPlotConfig("Refii.ClearPackets",
                    tracy::PlotFormatType::Number,
                    true,
                    false,
                    0x60A5FA);
    TracyPlotConfig("Refii.ResolvePackets",
                    tracy::PlotFormatType::Number,
                    true,
                    false,
                    0xF59E0B);
    TracyPlotConfig("Refii.PresentBlitPackets",
                    tracy::PlotFormatType::Number,
                    true,
                    false,
                    0x84CC16);
    TracyPlotConfig("Refii.ResourceEdges",
                    tracy::PlotFormatType::Number,
                    true,
                    false,
                    0x0EA5E9);
    TracyPlotConfig("Refii.ClearResourceEdges",
                    tracy::PlotFormatType::Number,
                    true,
                    false,
                    0x7DD3FC);
    TracyPlotConfig("Refii.ResolveResourceEdges",
                    tracy::PlotFormatType::Number,
                    true,
                    false,
                    0xFDBA74);
    TracyPlotConfig("Refii.SurfaceDestinationEdges",
                    tracy::PlotFormatType::Number,
                    true,
                    false,
                    0x34D399);
    TracyPlotConfig("Refii.PresentBlitEdges",
                    tracy::PlotFormatType::Number,
                    true,
                    false,
                    0xA3E635);
    TracyPlotConfig("Refii.PassResourceUsages",
                    tracy::PlotFormatType::Number,
                    true,
                    false,
                    0x4ADE80);
    TracyPlotConfig("Refii.PassClassified",
                    tracy::PlotFormatType::Number,
                    true,
                    false,
                    0x22C55E);
    TracyPlotConfig("Refii.PassDependencies",
                    tracy::PlotFormatType::Number,
                    true,
                    false,
                    0xFACC15);
    TracyPlotConfig("Refii.ResourceTransitions",
                    tracy::PlotFormatType::Number,
                    true,
                    false,
                    0xFB923C);
    TracyPlotConfig("Refii.FrameResourceLifetimes",
                    tracy::PlotFormatType::Number,
                    true,
                    false,
                    0xA78BFA);
    TracyPlotConfig("Refii.MaterialRecords",
                    tracy::PlotFormatType::Number,
                    true,
                    false,
                    0xF472B6);
    TracyPlotConfig("Refii.MeshRecords",
                    tracy::PlotFormatType::Number,
                    true,
                    false,
                    0xC084FC);
    TracyPlotConfig("Refii.AssetUsages",
                    tracy::PlotFormatType::Number,
                    true,
                    false,
                    0x2DD4BF);
    TracyPlotConfig("Refii.RenderGraphNodes",
                    tracy::PlotFormatType::Number,
                    true,
                    false,
                    0x22C55E);
    TracyPlotConfig("Refii.RenderGraphEdges",
                    tracy::PlotFormatType::Number,
                    true,
                    false,
                    0xFACC15);
    TracyPlotConfig("Refii.RenderGraphExecutionSteps",
                    tracy::PlotFormatType::Number,
                    true,
                    false,
                    0x14B8A6);
    TracyPlotConfig("Refii.RenderGraphBarriers",
                    tracy::PlotFormatType::Number,
                    true,
                    false,
                    0xFB923C);
    TracyPlotConfig("Refii.RenderGraphPhases",
                    tracy::PlotFormatType::Number,
                    true,
                    false,
                    0x06B6D4);
    TracyPlotConfig("Refii.RenderGraphRuntimeExecutions",
                    tracy::PlotFormatType::Number,
                    true,
                    false,
                    0x22C55E);
    TracyPlotConfig("Refii.RenderGraphBridgeExecutions",
                    tracy::PlotFormatType::Number,
                    true,
                    false,
                    0xF97316);
    TracyPlotConfig("Refii.RenderGraphSchedulerExecutions",
                    tracy::PlotFormatType::Number,
                    true,
                    false,
                    0xA3E635);
    TracyPlotConfig("Refii.RenderGraphSchedulerSteps",
                    tracy::PlotFormatType::Number,
                    true,
                    false,
                    0x84CC16);
    TracyPlotConfig("Refii.RenderGraphSchedulerBridgeSteps",
                    tracy::PlotFormatType::Number,
                    true,
                    false,
                    0xF59E0B);
    TracyPlotConfig("Refii.RenderGraphSchedulerGraphOwnedSteps",
                    tracy::PlotFormatType::Number,
                    true,
                    false,
                    0x60A5FA);
    TracyPlotConfig("Refii.RenderGraphSchedulerGraphOwnedPlanSteps",
                    tracy::PlotFormatType::Number,
                    true,
                    false,
                    0x38BDF8);
    TracyPlotConfig("Refii.RenderGraphSchedulerGraphOwnedRenderSteps",
                    tracy::PlotFormatType::Number,
                    true,
                    false,
                    0x818CF8);
    TracyPlotConfig("Refii.RenderGraphSchedulerGraphOwnedClearSteps",
                    tracy::PlotFormatType::Number,
                    true,
                    false,
                    0x67E8F9);
    TracyPlotConfig("Refii.RenderGraphSchedulerGraphOwnedClearCommands",
                    tracy::PlotFormatType::Number,
                    true,
                    false,
                    0x22D3EE);
    TracyPlotConfig("Refii.RenderGraphSchedulerGraphOwnedDrawSteps",
                    tracy::PlotFormatType::Number,
                    true,
                    false,
                    0xA78BFA);
    TracyPlotConfig("Refii.RenderGraphSchedulerGraphOwnedDrawCommands",
                    tracy::PlotFormatType::Number,
                    true,
                    false,
                    0xC084FC);
    TracyPlotConfig("Refii.RenderGraphSchedulerGraphOwnedResolveSteps",
                    tracy::PlotFormatType::Number,
                    true,
                    false,
                    0xF472B6);
    TracyPlotConfig("Refii.RenderGraphSchedulerGraphOwnedResolveCommands",
                    tracy::PlotFormatType::Number,
                    true,
                    false,
                    0xFB7185);
    TracyPlotConfig("Refii.RenderGraphSchedulerBridgeCommands",
                    tracy::PlotFormatType::Number,
                    true,
                    false,
                    0xFDBA74);
    TracyPlotConfig("Refii.RenderGraphRuntimePlanPasses",
                    tracy::PlotFormatType::Number,
                    true,
                    false,
                    0x4ADE80);
    TracyPlotConfig("Refii.RenderGraphRuntimePlanResources",
                    tracy::PlotFormatType::Number,
                    true,
                    false,
                    0x38BDF8);
    TracyPlotConfig("Refii.RenderGraphRuntimePlanEdgeOnlyPasses",
                    tracy::PlotFormatType::Number,
                    true,
                    false,
                    0x34D399);
    TracyPlotConfig("Refii.RenderGraphRuntimePlanSurfaceDestinationEdges",
                    tracy::PlotFormatType::Number,
                    true,
                    false,
                    0x2DD4BF);
    TracyPlotConfig("Refii.FrameHistoryEntries",
                    tracy::PlotFormatType::Number,
                    true,
                    false,
                    0x60A5FA);
    TracyPlotConfig("Refii.PacketQueueDepth",
                    tracy::PlotFormatType::Number,
                    true,
                    false,
                    0x14B8A6);
    TracyPlotConfig("Refii.PacketReplayPlanned",
                    tracy::PlotFormatType::Number,
                    true,
                    false,
                    0x22C55E);
    TracyPlotConfig("Refii.PacketReplayUnsupported",
                    tracy::PlotFormatType::Number,
                    true,
                    false,
                    0xEF4444);
    TracyPlotConfig("Refii.PipelineCreates",
                    tracy::PlotFormatType::Number,
                    true,
                    false,
                    0xFB7185);
    TracyPlotConfig("Refii.PipelineCacheMisses",
                    tracy::PlotFormatType::Number,
                    true,
                    false,
                    0xF43F5E);
    TracyPlotConfig("Refii.ShaderMisses",
                    tracy::PlotFormatType::Number,
                    true,
                    false,
                    0xF97316);
    TracyPlotConfig("Refii.TextureUploads",
                    tracy::PlotFormatType::Number,
                    true,
                    false,
                    0x2DD4BF);
    TracyPlotConfig("Refii.BufferUploads",
                    tracy::PlotFormatType::Number,
                    true,
                    false,
                    0x14B8A6);
    TracyPlotConfig("Refii.MissingResources",
                    tracy::PlotFormatType::Number,
                    true,
                    false,
                    0xEF4444);
    TracyPlotConfig("Refii.MissingTextures",
                    tracy::PlotFormatType::Number,
                    true,
                    false,
                    0xF87171);
    TracyPlotConfig("Refii.MissingShaders",
                    tracy::PlotFormatType::Number,
                    true,
                    false,
                    0xFBBF24);
    TracyPlotConfig("Refii.RenderTargetSwitches",
                    tracy::PlotFormatType::Number,
                    true,
                    false,
                    0x38BDF8);
    TracyPlotConfig("Refii.DepthTargetSwitches",
                    tracy::PlotFormatType::Number,
                    true,
                    false,
                    0x60A5FA);
    TracyPlotConfig("Refii.FrameDatabaseBytes",
                    tracy::PlotFormatType::Number,
                    true,
                    false,
                    0xA78BFA);
    TracyPlotConfig("Refii.RootCoverage.Serialized",
                    tracy::PlotFormatType::Number,
                    true,
                    false,
                    0x94A3B8);
    TracyPlotConfig("Refii.RootCoverage.Draw",
                    tracy::PlotFormatType::Number,
                    true,
                    false,
                    0x38BDF8);
    TracyPlotConfig("Refii.RootCoverage.VertexShader",
                    tracy::PlotFormatType::Number,
                    true,
                    false,
                    0x818CF8);
    TracyPlotConfig("Refii.RootCoverage.PixelShader",
                    tracy::PlotFormatType::Number,
                    true,
                    false,
                    0xA78BFA);
    TracyPlotConfig("Refii.RootCoverage.FullShader",
                    tracy::PlotFormatType::Number,
                    true,
                    false,
                    0x22C55E);
    TracyPlotConfig("Refii.RootCoverage.PrivateWrapper",
                    tracy::PlotFormatType::Number,
                    true,
                    false,
                    0xF97316);
    TracyPlotConfig("Refii.RootCoverage.Any",
                    tracy::PlotFormatType::Number,
                    true,
                    false,
                    0x06B6D4);
    TracyPlotConfig("Refii.Pass.MainScene",
                    tracy::PlotFormatType::Number,
                    true,
                    false,
                    0x22C55E);
    TracyPlotConfig("Refii.Pass.Video",
                    tracy::PlotFormatType::Number,
                    true,
                    false,
                    0xA78BFA);
    TracyPlotConfig("Refii.Pass.PrivateRect",
                    tracy::PlotFormatType::Number,
                    true,
                    false,
                    0xF97316);
    TracyPlotConfig("Refii.Pass.DepthOnly",
                    tracy::PlotFormatType::Number,
                    true,
                    false,
                    0x60A5FA);
    TracyPlotConfig("Refii.Pass.PostProcessOrUI",
                    tracy::PlotFormatType::Number,
                    true,
                    false,
                    0x06B6D4);
    TracyPlotConfig("Refii.Pass.WrapperDraw",
                    tracy::PlotFormatType::Number,
                    true,
                    false,
                    0xFACC15);
  });
}

bool ShouldLogPlumeFrameTiming(uint32_t frame_index) {
  // Every 120 frames is far too sparse to characterise the title phase: at
  // ~4fps a 34-second title window yielded exactly TWO samples (1.2ms with 0
  // draws, 88.6ms with 164 draws). That is not a distribution, and reading the
  // larger one as "the compose cost" is exactly the kind of single-sample
  // conclusion this project keeps getting burned by.
  //
  // 8 is dense enough to characterise a 4fps phase (~30 samples per 60s) and
  // still ~15x cheaper than logging every frame. Restore 120 once the compose
  // distribution is established.
  return frame_index <= 8 || frame_index % 8 == 0;
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

std::string BuildFrameCorrelationLabel(
    uint32_t frame_index, uint32_t width, uint32_t height,
    const FableDirectRendererStats& direct_stats,
    const RendererHookObserverSnapshot& hook_snapshot) {
  char label[256] = {};
  std::snprintf(
      label, sizeof(label),
      "Frame %u / DrawPackets %u / DrawEvents %u / VS_%08X / PS_%08X / "
      "RT_%08X / Pix_%08X / Output_%ux%u",
      frame_index, direct_stats.draw_count, direct_stats.last_draw_events,
      hook_snapshot.vertex_shader, hook_snapshot.pixel_shader,
      direct_stats.last_present_target, hook_snapshot.last_pix_texture, width,
      height);
  return label;
}

std::string BuildRenderDocCaptureTitle(std::string_view correlation_label) {
  return "Refii Plume / " + std::string(correlation_label);
}

std::string BuildRenderDocCaptureFileTag(uint32_t frame_index,
                                         uint32_t draw_index,
                                         std::string_view draw_target,
                                         std::string_view pass_name,
                                         std::string_view capture_reason) {
  char prefix[160] = {};
  if (!capture_reason.empty()) {
    const bool error_reason =
        capture_reason.find("error") != std::string_view::npos ||
        capture_reason.find("failed") != std::string_view::npos ||
        capture_reason.find("failure") != std::string_view::npos;
    std::snprintf(prefix, sizeof(prefix),
                  error_reason ? "refii_error_%.*s_frame_%06u"
                               : "refii_%.*s_frame_%06u",
                  static_cast<int>(capture_reason.size()),
                  capture_reason.data(), frame_index);
  } else {
    std::snprintf(prefix, sizeof(prefix), "refii_frame_%06u", frame_index);
  }

  std::string tag(prefix);
  if (draw_index != 0) {
    char draw_label[32] = {};
    std::snprintf(draw_label, sizeof(draw_label), "_draw_%06u", draw_index);
    tag += draw_label;
  }
  if (!draw_target.empty()) {
    tag += "_target_";
    tag.append(SanitizeRenderDocTagComponent(draw_target));
  }
  if (!pass_name.empty()) {
    tag += "_pass_";
    tag.append(SanitizeRenderDocTagComponent(pass_name));
  }
  return tag;
}

std::string BuildFrameDebugScopeLabel(uint32_t frame_index, uint32_t width,
                                      uint32_t height) {
  char label[96] = {};
  std::snprintf(label, sizeof(label), "RefiiPlume Frame %u Output_%ux%u",
                frame_index, width, height);
  return label;
}






#if REFII_RENDERER_HAS_PLUME
struct PlumeFrameContext {
  bool submitted = false;
  std::unique_ptr<plume::RenderCommandList> command_list;
  std::unique_ptr<plume::RenderCommandSemaphore> acquire_semaphore;
  std::unique_ptr<plume::RenderCommandFence> fence;
};

// Present-decoupling step 2: persistent offscreen compose target. The fable
// frame records into one of these instead of the acquired swapchain image;
// the finished compose is then blitted into the swapchain image, and the
// diagnostics/ImGui overlay draw into the swapchain image on top exactly as
// they previously drew after the scene. Two targets alternate so a re-present
// blit of one can overlap a record into the other (step 3 consumes this);
// last_use_frame_context names the frame context whose fence covers this
// target's most recent GPU use. Same size/format as the swapchain images,
// recreated with the framebuffers on any resize.
struct PlumeComposeTarget {
  std::unique_ptr<plume::RenderTexture> color_texture;
  // Color-only framebuffer (mirrors framebuffers[i] for the swapchain image).
  std::unique_ptr<plume::RenderFramebuffer> framebuffer;
  // Color + shared backend depth (mirrors depth_framebuffers[i]).
  std::unique_ptr<plume::RenderFramebuffer> depth_framebuffer;
  uint32_t last_use_frame_context = UINT32_MAX;
  bool recorded = false;
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
  bool logged_renderdoc_pass_watch = false;
  bool logged_renderdoc_draw_watch = false;
  uint32_t last_renderdoc_draw_capture = 0;
  std::string last_renderdoc_draw_target_capture;
  std::string last_renderdoc_pass_capture;
  // Remaining consecutive captures after a pass match: the queued-command
  // probe sees commands one frame before/after they execute, so a single
  // capture often brackets only the held-present blit.
  uint32_t renderdoc_pass_capture_repeats = 0;
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
  // Present-decoupling steps 2/3 (see PlumeComposeTarget). next_compose_target
  // is the target the next full record uses; last_recorded_compose_target is
  // the most recent completed record (UINT32_MAX = none yet, forces a record).
  // last_record_present_target / last_record_bink_upload_count snapshot what
  // that record saw, for the fast path's PresentFableDirectFrame call and the
  // "fresh Bink texels force a record" check.
  std::array<PlumeComposeTarget, 2> compose_targets;
  uint32_t next_compose_target = 0;
  uint32_t last_recorded_compose_target = UINT32_MAX;
  uint32_t last_record_present_target = 0;
  uint64_t last_record_bink_upload_count = 0;
  uint64_t last_record_deferred_capture_generation = 0;
  // When the last executed record finished (submit-side bookkeeping). Gates
  // ONLY the captures record reason — see kPlumeCapturesRerecordMinInterval.
  PlumeClock::time_point last_record_completed_at{};
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
std::atomic<uint32_t> g_plume_backend_debug_status{
    static_cast<uint32_t>(PlumeBackendStatus::kUnavailable)};
std::atomic<uint32_t> g_plume_backend_debug_api{
    static_cast<uint32_t>(RendererGraphicsApi::kVulkan)};
std::atomic_bool g_plume_backend_debug_attempted{false};
std::atomic_bool g_plume_backend_debug_direct_queue_ready{false};
std::atomic_bool g_plume_backend_debug_copy_queue_ready{false};
std::atomic_bool g_plume_backend_debug_swapchain_ready{false};
std::atomic_bool g_plume_backend_debug_swapchain_present_wait_enabled{false};
std::atomic<uint32_t> g_plume_backend_debug_next_frame_context{0};
std::atomic<uint32_t> g_plume_backend_debug_frame_context_count{0};
std::atomic<uint32_t> g_plume_backend_debug_release_semaphore_count{0};
std::atomic<uint32_t> g_plume_backend_debug_framebuffer_count{0};
std::atomic<uint32_t> g_plume_backend_debug_depth_framebuffer_count{0};
std::atomic<uint64_t> g_plume_backend_debug_dedicated_video_memory{0};
std::mutex g_plume_backend_debug_device_name_mutex;
std::array<char, kPlumeBackendDebugDeviceNameCapacity>
    g_plume_backend_debug_device_name{};
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
// Present census (present-decoupling step 1): cumulative counters logged
// every ~2s from the present path. requests = pump presents that reached the
// UI-thread coalescer; coalesced_dropped = requests discarded because one was
// already queued (g_plume_async_present_pending held — with the step-5
// pipeline cvar ON the slot is released when execution starts, so this counts
// only requests faster than one-queued-plus-one-executing); presented =
// swapchain presents that returned true; rerecords = frames that ran the full
// fable record; represents = step-3 fast-path frames that re-blitted the held
// offscreen compose target instead.
std::atomic<uint64_t> g_plume_present_census_requests{0};
std::atomic<uint64_t> g_plume_present_census_coalesced_dropped{0};
std::atomic<uint64_t> g_plume_present_census_presented{0};
std::atomic<uint64_t> g_plume_present_census_rerecords{0};
std::atomic<uint64_t> g_plume_present_census_represents{0};
// Per-reason attribution for every executed record (first matching reason in
// evaluation order wins; a fast-path frame increments nothing). Run 460:
// rerecords outnumbered guest frames ~10x with no attribution — this names
// the residual false-positive source directly on the census line.
enum PlumePresentRecordWhy : size_t {
  kPlumeRecordWhyNoCache = 0,   // no completed compose target (boot/resize)
  kPlumeRecordWhyRenderDoc,     // a capture wants this present
  kPlumeRecordWhyBink,          // Bink upload generation advanced
  kPlumeRecordWhyCaptures,      // deferred-capture enqueue generation advanced
  kPlumeRecordWhyMode,          // GTL-authoritative submission off
  kPlumeRecordWhyVideo,         // startup-video pump active
  kPlumeRecordWhyBoundary,      // guest frame boundary missing or advanced
  kPlumeRecordWhyCarried,       // carried draw packets await their compose
  kPlumeRecordWhyPackets,       // late GTL packets below the held boundary
  kPlumeRecordWhyCount,
};
std::array<std::atomic<uint64_t>, kPlumeRecordWhyCount>
    g_plume_present_record_why{};
// Wakes where the captures reason WANTED a record but was suppressed by the
// minimum re-record interval (run 461: the guest enqueues ~2 captures spread
// across each ~190ms frame, so a ~100ms record always overlapped a fresh
// enqueue and captures-caused records ran back-to-back, freezing represents).
std::atomic<uint64_t> g_plume_present_census_captures_deferred{0};

struct AtomicPlumeFrameHistoryEntry {
  std::atomic_bool valid{false};
  std::atomic_bool presented{false};
  std::atomic_bool renderdoc_capture_active{false};
  std::atomic<uint32_t> frame_index{0};
  std::atomic<uint32_t> width{0};
  std::atomic<uint32_t> height{0};
  std::atomic<uint32_t> draw_count{0};
  std::atomic<uint32_t> indexed_draw_count{0};
  std::atomic<uint32_t> render_command_count{0};
  std::atomic<uint32_t> resource_upload_count{0};
  std::atomic<uint32_t> pipeline_cache_miss_count{0};
  std::atomic<uint32_t> failed_resource_sync_count{0};
  std::atomic<uint32_t> present_target{0};
  std::atomic<int64_t> frame_total_us{0};
  std::atomic<int64_t> direct_us{0};
  std::atomic<int64_t> submit_us{0};
  std::atomic<int64_t> present_us{0};
};

std::array<AtomicPlumeFrameHistoryEntry, kPlumeFrameHistoryCapacity>
    g_plume_frame_history;
std::atomic<uint64_t> g_plume_frame_history_write_count{0};

void StorePlumeFrameHistoryEntry(
    uint32_t frame_index, uint32_t width, uint32_t height, bool presented,
    bool renderdoc_capture_active, const FableDirectRendererStats& direct_stats,
    int64_t frame_total_us, int64_t direct_us, int64_t submit_us,
    int64_t present_us) {
  const uint64_t write_index =
      g_plume_frame_history_write_count.fetch_add(1, std::memory_order_acq_rel);
  AtomicPlumeFrameHistoryEntry& entry =
      g_plume_frame_history[write_index % kPlumeFrameHistoryCapacity];
  entry.valid.store(false, std::memory_order_release);
  entry.presented.store(presented, std::memory_order_relaxed);
  entry.renderdoc_capture_active.store(renderdoc_capture_active,
                                       std::memory_order_relaxed);
  entry.frame_index.store(frame_index, std::memory_order_relaxed);
  entry.width.store(width, std::memory_order_relaxed);
  entry.height.store(height, std::memory_order_relaxed);
  entry.draw_count.store(direct_stats.draw_count, std::memory_order_relaxed);
  entry.indexed_draw_count.store(direct_stats.indexed_draw_count,
                                 std::memory_order_relaxed);
  entry.render_command_count.store(direct_stats.last_render_commands,
                                   std::memory_order_relaxed);
  entry.resource_upload_count.store(direct_stats.resource_uploads,
                                    std::memory_order_relaxed);
  entry.pipeline_cache_miss_count.store(direct_stats.pipeline_cache_misses,
                                        std::memory_order_relaxed);
  entry.failed_resource_sync_count.store(direct_stats.failed_resource_syncs,
                                         std::memory_order_relaxed);
  entry.present_target.store(direct_stats.last_present_target,
                             std::memory_order_relaxed);
  entry.frame_total_us.store(frame_total_us, std::memory_order_relaxed);
  entry.direct_us.store(direct_us, std::memory_order_relaxed);
  entry.submit_us.store(submit_us, std::memory_order_relaxed);
  entry.present_us.store(present_us, std::memory_order_relaxed);
  entry.valid.store(true, std::memory_order_release);
}

PlumeFrameHistoryEntry LoadPlumeFrameHistoryEntry(
    const AtomicPlumeFrameHistoryEntry& entry) {
  PlumeFrameHistoryEntry snapshot;
  snapshot.valid = entry.valid.load(std::memory_order_acquire);
  snapshot.presented = entry.presented.load(std::memory_order_relaxed);
  snapshot.renderdoc_capture_active =
      entry.renderdoc_capture_active.load(std::memory_order_relaxed);
  snapshot.frame_index = entry.frame_index.load(std::memory_order_relaxed);
  snapshot.width = entry.width.load(std::memory_order_relaxed);
  snapshot.height = entry.height.load(std::memory_order_relaxed);
  snapshot.draw_count = entry.draw_count.load(std::memory_order_relaxed);
  snapshot.indexed_draw_count =
      entry.indexed_draw_count.load(std::memory_order_relaxed);
  snapshot.render_command_count =
      entry.render_command_count.load(std::memory_order_relaxed);
  snapshot.resource_upload_count =
      entry.resource_upload_count.load(std::memory_order_relaxed);
  snapshot.pipeline_cache_miss_count =
      entry.pipeline_cache_miss_count.load(std::memory_order_relaxed);
  snapshot.failed_resource_sync_count =
      entry.failed_resource_sync_count.load(std::memory_order_relaxed);
  snapshot.present_target = entry.present_target.load(std::memory_order_relaxed);
  snapshot.frame_total_us = entry.frame_total_us.load(std::memory_order_relaxed);
  snapshot.direct_us = entry.direct_us.load(std::memory_order_relaxed);
  snapshot.submit_us = entry.submit_us.load(std::memory_order_relaxed);
  snapshot.present_us = entry.present_us.load(std::memory_order_relaxed);
  return snapshot;
}

void StorePlumeBackendDebugSnapshotLocked() {
  g_plume_backend_debug_status.store(
      static_cast<uint32_t>(g_plume_backend.status), std::memory_order_relaxed);
  g_plume_backend_debug_api.store(static_cast<uint32_t>(g_plume_backend.api),
                                  std::memory_order_relaxed);
  g_plume_backend_debug_attempted.store(g_plume_backend.attempted,
                                        std::memory_order_relaxed);

#if REFII_RENDERER_HAS_PLUME
  g_plume_backend_debug_direct_queue_ready.store(
      g_plume_backend.direct_queue != nullptr, std::memory_order_relaxed);
  g_plume_backend_debug_copy_queue_ready.store(
      g_plume_backend.copy_queue != nullptr, std::memory_order_relaxed);
  g_plume_backend_debug_swapchain_ready.store(
      g_plume_backend.swap_chain != nullptr, std::memory_order_relaxed);
  g_plume_backend_debug_swapchain_present_wait_enabled.store(
      g_plume_backend.swap_chain_present_wait_enabled,
      std::memory_order_relaxed);
  g_plume_backend_debug_next_frame_context.store(
      g_plume_backend.next_frame_context, std::memory_order_relaxed);
  g_plume_backend_debug_frame_context_count.store(
      static_cast<uint32_t>(g_plume_backend.frame_contexts.size()),
      std::memory_order_relaxed);
  g_plume_backend_debug_release_semaphore_count.store(
      static_cast<uint32_t>(g_plume_backend.release_semaphores.size()),
      std::memory_order_relaxed);
  g_plume_backend_debug_framebuffer_count.store(
      static_cast<uint32_t>(g_plume_backend.framebuffers.size()),
      std::memory_order_relaxed);
  g_plume_backend_debug_depth_framebuffer_count.store(
      static_cast<uint32_t>(g_plume_backend.depth_framebuffers.size()),
      std::memory_order_relaxed);

  std::string device_name;
  uint64_t dedicated_video_memory = 0;
  if (g_plume_backend.device) {
    const plume::RenderDeviceDescription& description =
        g_plume_backend.device->getDescription();
    device_name = description.name;
    dedicated_video_memory = description.dedicatedVideoMemory;
  }
  g_plume_backend_debug_dedicated_video_memory.store(
      dedicated_video_memory, std::memory_order_relaxed);
  {
    std::lock_guard<std::mutex> name_lock(
        g_plume_backend_debug_device_name_mutex);
    g_plume_backend_debug_device_name.fill('\0');
    const size_t copy_size =
        std::min<size_t>(device_name.size(),
                         kPlumeBackendDebugDeviceNameCapacity - 1);
    std::memcpy(g_plume_backend_debug_device_name.data(),
                device_name.data(), copy_size);
  }
#else
  g_plume_backend_debug_direct_queue_ready.store(false,
                                                 std::memory_order_relaxed);
  g_plume_backend_debug_copy_queue_ready.store(false, std::memory_order_relaxed);
  g_plume_backend_debug_swapchain_ready.store(false, std::memory_order_relaxed);
  g_plume_backend_debug_swapchain_present_wait_enabled.store(
      false, std::memory_order_relaxed);
  g_plume_backend_debug_next_frame_context.store(0, std::memory_order_relaxed);
  g_plume_backend_debug_frame_context_count.store(0, std::memory_order_relaxed);
  g_plume_backend_debug_release_semaphore_count.store(
      0, std::memory_order_relaxed);
  g_plume_backend_debug_framebuffer_count.store(0, std::memory_order_relaxed);
  g_plume_backend_debug_depth_framebuffer_count.store(
      0, std::memory_order_relaxed);
  g_plume_backend_debug_dedicated_video_memory.store(
      0, std::memory_order_relaxed);
  {
    std::lock_guard<std::mutex> name_lock(
        g_plume_backend_debug_device_name_mutex);
    g_plume_backend_debug_device_name.fill('\0');
  }
#endif
}

#if REFII_RENDERER_HAS_PLUME
bool DrainPlumeBackendGpuLocked();

void StorePlumeOutputSnapshotLocked() {
  StorePlumeBackendDebugSnapshotLocked();
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

void ResetPlumeComposeTargetsLocked() {
  for (PlumeComposeTarget& target : g_plume_backend.compose_targets) {
    target.color_texture.reset();
    target.framebuffer.reset();
    target.depth_framebuffer.reset();
    target.last_use_frame_context = UINT32_MAX;
    target.recorded = false;
  }
  g_plume_backend.next_compose_target = 0;
  g_plume_backend.last_recorded_compose_target = UINT32_MAX;
  g_plume_backend.last_record_present_target = 0;
}

void ResetPlumeOutputLocked() {
  DrainPlumeBackendGpuLocked();
  StorePlumeOutputSnapshotLocked();
  g_plume_backend.framebuffers.clear();
  g_plume_backend.depth_framebuffers.clear();
  g_plume_backend.depth_texture.reset();
  ResetPlumeComposeTargetsLocked();
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
  // The stable-tree shadow exposes SDLWindow::window(); the netplay SDK's
  // WindowSDL exposes sdl_window() (shadow-added accessor).
#if defined(REFII_REXGLUE_NETPLAY) && REFII_REXGLUE_NETPLAY
  auto* sdl_window = dynamic_cast<rex::ui::WindowSDL*>(window);
  SDL_Window* sdl_window_handle = sdl_window ? sdl_window->sdl_window() : nullptr;
#else
  auto* sdl_window = dynamic_cast<rex::ui::SDLWindow*>(window);
  SDL_Window* sdl_window_handle = sdl_window ? sdl_window->window() : nullptr;
#endif
  if (!sdl_window_handle) {
    REXLOG_WARN(
        "Refii plume/vulkan output could not access the ReXGlue SDL window");
    return false;
  }
  render_window = sdl_window_handle;
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
  ResetPlumeComposeTargetsLocked();
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
    plume::RenderTexture* color_attachment =
        g_plume_backend.swap_chain->getTexture(i);
    if (color_attachment) {
      color_attachment->setName("Refii Plume Output Swapchain " +
                                std::to_string(i));
    }
    const plume::RenderTexture* color_attachment_const = color_attachment;
    plume::RenderFramebufferDesc framebuffer_desc;
    framebuffer_desc.colorAttachments = &color_attachment_const;
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
    depth_framebuffer_desc.colorAttachments = &color_attachment_const;
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

  // Present-decoupling step 2: the offscreen compose targets, same size and
  // format as the swapchain images (created as B8G8R8A8_UNORM in
  // EnsurePlumeOutputLocked). Recreated with the framebuffers on any resize,
  // which also clears the recorded flags so the first post-resize present
  // takes the record path.
  for (size_t i = 0; i < g_plume_backend.compose_targets.size(); ++i) {
    PlumeComposeTarget& target = g_plume_backend.compose_targets[i];
    target.color_texture =
        g_plume_backend.device->createTexture(plume::RenderTextureDesc::ColorTarget(
            width, height, plume::RenderFormat::B8G8R8A8_UNORM));
    if (!target.color_texture) {
      REXLOG_WARN(
          "Refii plume output could not create compose target {} {}x{}", i,
          width, height);
      g_plume_backend.framebuffers.clear();
      g_plume_backend.depth_framebuffers.clear();
      g_plume_backend.depth_texture.reset();
      ResetPlumeComposeTargetsLocked();
      return false;
    }
    target.color_texture->setName("Refii Plume Compose Target " +
                                  std::to_string(i));
    const plume::RenderTexture* compose_attachment = target.color_texture.get();
    plume::RenderFramebufferDesc compose_framebuffer_desc;
    compose_framebuffer_desc.colorAttachments = &compose_attachment;
    compose_framebuffer_desc.colorAttachmentsCount = 1;
    target.framebuffer =
        g_plume_backend.device->createFramebuffer(compose_framebuffer_desc);

    plume::RenderFramebufferDesc compose_depth_framebuffer_desc;
    compose_depth_framebuffer_desc.colorAttachments = &compose_attachment;
    compose_depth_framebuffer_desc.colorAttachmentsCount = 1;
    compose_depth_framebuffer_desc.depthAttachment =
        g_plume_backend.depth_texture.get();
    target.depth_framebuffer = g_plume_backend.device->createFramebuffer(
        compose_depth_framebuffer_desc);
    if (!target.framebuffer || !target.depth_framebuffer) {
      REXLOG_WARN(
          "Refii plume output could not create compose framebuffers {} {}x{}",
          i, width, height);
      g_plume_backend.framebuffers.clear();
      g_plume_backend.depth_framebuffers.clear();
      g_plume_backend.depth_texture.reset();
      ResetPlumeComposeTargetsLocked();
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
    case PlumeFableCommandSource::kStaged:
      return "staged";
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
  StorePlumeBackendDebugSnapshotLocked();
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
  StorePlumeBackendDebugSnapshotLocked();

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
    StorePlumeBackendDebugSnapshotLocked();
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
    StorePlumeBackendDebugSnapshotLocked();
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
    StorePlumeBackendDebugSnapshotLocked();
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
    StorePlumeBackendDebugSnapshotLocked();
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
  StorePlumeBackendDebugSnapshotLocked();
  REXLOG_INFO(
      "Refii plume/{} backend initialized device=\"{}\" vram={} MiB "
      "direct_queue=ready copy_queue={}",
      PlumeApiBridgeName(api), description.name,
      description.dedicatedVideoMemory / (1024 * 1024),
      g_plume_backend.copy_queue ? "ready" : "direct_queue_uploads");
  return g_plume_backend.status;
#else
  g_plume_backend.status = PlumeBackendStatus::kUnavailable;
  StorePlumeBackendDebugSnapshotLocked();
  return g_plume_backend.status;
#endif
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

// Live swapchain extent, lock-free. This is the resolution we actually present
// at, which is what "render at the requested resolution" has to mean -- the
// video_mode_width/height cvars DEFAULT to 1280x720 (rexglue window.cpp), so
// reading them alone makes the render-surface promotion a no-op on any machine
// that never set them, which is why the title stayed 720p. Cheap enough for the
// per-resource decode path: two relaxed atomic loads, no lock, no snapshot copy.
bool CurrentPlumeOutputExtent(uint32_t &width, uint32_t &height) {
  const uint32_t w = g_plume_output_width.load(std::memory_order_relaxed);
  const uint32_t h = g_plume_output_height.load(std::memory_order_relaxed);
  if (w == 0 || h == 0) {
    return false;
  }
  width = w;
  height = h;
  return true;
}

PlumeBackendDebugSnapshot GetPlumeBackendDebugSnapshot() {
  PlumeBackendDebugSnapshot snapshot;
  snapshot.status = static_cast<PlumeBackendStatus>(
      g_plume_backend_debug_status.load(std::memory_order_relaxed));
  snapshot.api = static_cast<RendererGraphicsApi>(
      g_plume_backend_debug_api.load(std::memory_order_relaxed));
  snapshot.attempted =
      g_plume_backend_debug_attempted.load(std::memory_order_relaxed);
  snapshot.direct_queue_ready =
      g_plume_backend_debug_direct_queue_ready.load(std::memory_order_relaxed);
  snapshot.copy_queue_ready =
      g_plume_backend_debug_copy_queue_ready.load(std::memory_order_relaxed);
  snapshot.swapchain_ready =
      g_plume_backend_debug_swapchain_ready.load(std::memory_order_relaxed);
  snapshot.swapchain_present_wait_enabled =
      g_plume_backend_debug_swapchain_present_wait_enabled.load(
          std::memory_order_relaxed);
  snapshot.next_frame_context =
      g_plume_backend_debug_next_frame_context.load(std::memory_order_relaxed);
  snapshot.frame_context_count =
      g_plume_backend_debug_frame_context_count.load(std::memory_order_relaxed);
  snapshot.release_semaphore_count =
      g_plume_backend_debug_release_semaphore_count.load(
          std::memory_order_relaxed);
  snapshot.framebuffer_count =
      g_plume_backend_debug_framebuffer_count.load(std::memory_order_relaxed);
  snapshot.depth_framebuffer_count =
      g_plume_backend_debug_depth_framebuffer_count.load(
          std::memory_order_relaxed);
  snapshot.dedicated_video_memory =
      g_plume_backend_debug_dedicated_video_memory.load(
          std::memory_order_relaxed);
  {
    std::lock_guard<std::mutex> name_lock(
        g_plume_backend_debug_device_name_mutex);
    snapshot.device_name = g_plume_backend_debug_device_name;
  }
  return snapshot;
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

PlumeFrameHistorySnapshot GetPlumeFrameHistorySnapshot() {
  PlumeFrameHistorySnapshot snapshot;
  const uint64_t written =
      g_plume_frame_history_write_count.load(std::memory_order_acquire);
  const uint32_t available = static_cast<uint32_t>(
      std::min<uint64_t>(written, kPlumeFrameHistoryCapacity));
  for (uint32_t index = 0; index < available; ++index) {
    const uint64_t history_index = written - 1u - index;
    const AtomicPlumeFrameHistoryEntry& source =
        g_plume_frame_history[history_index % kPlumeFrameHistoryCapacity];
    PlumeFrameHistoryEntry entry = LoadPlumeFrameHistoryEntry(source);
    if (!entry.valid) {
      continue;
    }
    snapshot.entries[snapshot.count++] = entry;
  }
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
  // Present-decoupling step 5 (2026-07-30): the real present body stays on
  // the UI thread DELIBERATELY. The Vulkan side would be safe from the pump
  // thread — swapchain, surface, queue and every frame resource are touched
  // only under g_plume_backend.mutex, resize is handled inside the present
  // body via swap_chain->needsResize(), and vkQueuePresentKHR has no
  // thread-affinity requirement — but the ImGui overlay is not: it renders on
  // the SHARED rexglue ImGui context (EnsureImGuiContext ->
  // g_rexglue_imgui_drawer->context()), and that context's io state is
  // written lock-free by the UI thread's input listeners (ImGuiDrawer
  // OnMouseDown/OnMouseMove/OnKey*, imgui_drawer.cpp). NewFrame/Render racing
  // those writes is a data race today only prevented by both running on the
  // UI thread. So instead of moving the present, the single-slot coalescer is
  // re-armed when the queued present STARTS executing (cvar-gated below):
  // back-to-back presents pipeline (at most one queued behind the one
  // executing) instead of dropping every request that arrives mid-execution.
  // The UI-loop wake per request already exists — CallInUIThreadDeferred
  // pushes an SDL user event (SDLWindowedAppContext::
  // NotifyUILoopOfPendingFunctions -> SDL_PushEvent).
  if (ShouldMarshalPlumeToUIThread()) {
    ZoneScopedN("RefiiPlume/PresentFrameMarshalToUI");
    if (drive_fable_frame) {
      g_plume_present_census_requests.fetch_add(1, std::memory_order_relaxed);
      bool expected = false;
      if (!g_plume_async_present_pending.compare_exchange_strong(
              expected, true, std::memory_order_acq_rel)) {
        g_plume_present_census_coalesced_dropped.fetch_add(
            1, std::memory_order_relaxed);
        if (!g_logged_plume_async_present_coalesced.exchange(
                true, std::memory_order_relaxed)) {
          REXLOG_INFO(
              "Refii plume present pump is coalescing pending UI-thread "
              "presents");
        }
        return false;
      }

      const bool pipeline_presents = RendererPresentUIThreadPipelineEnabled();
      if (!CallPlumeOnUIThreadDeferred([drive_fable_frame,
                                        pipeline_presents]() {
            if (pipeline_presents) {
              // Release the slot at execution START so the next pump wake can
              // queue one present behind this one; anything faster than that
              // still coalesces into the single queued slot.
              g_plume_async_present_pending.store(false,
                                                  std::memory_order_release);
            }
            if (!g_plume_backend_shutdown_requested.load(
                    std::memory_order_acquire)) {
              PresentPlumeFrame(drive_fable_frame);
            }
            if (!pipeline_presents) {
              g_plume_async_present_pending.store(false,
                                                  std::memory_order_release);
            }
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
    const bool staged_has_render_work =
        direct_snapshot.staged_clear_command_count != 0 ||
        direct_snapshot.staged_resolve_command_count != 0 ||
        std::any_of(direct_snapshot.staged_draw_source_counts.begin(),
                    direct_snapshot.staged_draw_source_counts.end(),
                    [](uint32_t count) { return count != 0; });
    if (direct_snapshot.submitted_render_command_count != 0) {
      command_source = PlumeFableCommandSource::kSubmitted;
    } else if (staged_has_render_work) {
      command_source = PlumeFableCommandSource::kStaged;
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
      direct_snapshot.last_command_boundary_staged_draws != 0;
  const uint32_t renderdoc_capture_frame_index =
      g_plume_backend.present_count + 1u;
  const uint32_t renderdoc_capture_frame = RenderDocCaptureFrameIndex();
  if (renderdoc_capture_frame != 0 &&
      renderdoc_capture_frame_index == renderdoc_capture_frame) {
    RequestRenderDocFrameCapture();
  }
  const uint32_t renderdoc_capture_draw_index = RenderDocCaptureDrawIndex();
  if (renderdoc_capture_draw_index == 0) {
    g_plume_backend.last_renderdoc_draw_capture = 0;
    g_plume_backend.logged_renderdoc_draw_watch = false;
  } else if (g_plume_backend.last_renderdoc_draw_capture !=
             renderdoc_capture_draw_index) {
    if (!g_plume_backend.logged_renderdoc_draw_watch) {
      REXLOG_INFO("Refii plume RenderDoc draw watch active draw={}",
                  renderdoc_capture_draw_index);
      g_plume_backend.logged_renderdoc_draw_watch = true;
    }
    constexpr size_t kMaxRenderDocDrawCommandProbe = 4096;
    if (FableDirectQueuedDrawPresent(renderdoc_capture_draw_index,
                                     kMaxRenderDocDrawCommandProbe)) {
      if (RequestRenderDocFrameCapture()) {
        g_plume_backend.last_renderdoc_draw_capture =
            renderdoc_capture_draw_index;
        REXLOG_INFO(
            "Refii plume RenderDoc draw capture armed draw={} present={}",
            renderdoc_capture_draw_index, renderdoc_capture_frame_index);
      }
    }
  }
  const std::string renderdoc_capture_draw_target =
      RenderDocDeepObserverCaptureDrawTarget();
  if (renderdoc_capture_draw_target.empty()) {
    g_plume_backend.last_renderdoc_draw_target_capture.clear();
    g_plume_backend.logged_renderdoc_draw_target_watch = false;
  } else if (g_plume_backend.last_renderdoc_draw_target_capture !=
                 renderdoc_capture_draw_target) {
    std::string_view renderdoc_hook_root_token;
    if (!g_plume_backend.logged_renderdoc_draw_target_watch) {
      REXLOG_INFO("Refii plume RenderDoc draw-target watch active target={}",
                  renderdoc_capture_draw_target);
      g_plume_backend.logged_renderdoc_draw_target_watch = true;
    }
    if (IsRenderDocResolveCaptureTarget(renderdoc_capture_draw_target)) {
      REXLOG_INFO(
          "Refii plume RenderDoc resolve-target watch is record-time only "
          "target={}",
          renderdoc_capture_draw_target);
      g_plume_backend.last_renderdoc_draw_target_capture =
          renderdoc_capture_draw_target;
    } else if (IsRenderDocSurfaceDestinationCaptureTarget(
                   renderdoc_capture_draw_target)) {
      REXLOG_INFO(
          "Refii plume RenderDoc surface-destination target watch is "
          "record-time only target={}",
          renderdoc_capture_draw_target);
      g_plume_backend.last_renderdoc_draw_target_capture =
          renderdoc_capture_draw_target;
    } else if (IsRenderDocRootCoverageCaptureTarget(
                   renderdoc_capture_draw_target)) {
      if (renderdoc_ready_to_capture && RequestRenderDocFrameCapture()) {
        g_plume_backend.last_renderdoc_draw_target_capture =
            renderdoc_capture_draw_target;
        REXLOG_INFO(
            "Refii plume RenderDoc root-coverage capture armed target={} "
            "present={}",
            renderdoc_capture_draw_target, renderdoc_capture_frame_index);
      }
    } else if (TryGetRenderDocHookRootCaptureToken(
                   renderdoc_capture_draw_target,
                   renderdoc_hook_root_token)) {
      if (RenderDocHookRootTargetActive(renderdoc_hook_root_token) &&
          RequestRenderDocFrameCapture()) {
        g_plume_backend.last_renderdoc_draw_target_capture =
            renderdoc_capture_draw_target;
        REXLOG_INFO(
            "Refii plume RenderDoc hook-root capture armed target={} "
            "root={} present={}",
            renderdoc_capture_draw_target, renderdoc_hook_root_token,
            renderdoc_capture_frame_index);
      }
    } else if (QueuedCommandsContainRenderDocDrawTarget(
                   renderdoc_capture_draw_target)) {
      if (RequestRenderDocFrameCapture()) {
        g_plume_backend.last_renderdoc_draw_target_capture =
            renderdoc_capture_draw_target;
        REXLOG_INFO(
            "Refii plume RenderDoc draw-target capture armed target={} "
            "present={}",
            renderdoc_capture_draw_target, renderdoc_capture_frame_index);
      }
    }
  }
  const std::string renderdoc_capture_pass = RenderDocCapturePassName();
  if (renderdoc_capture_pass.empty()) {
    g_plume_backend.last_renderdoc_pass_capture.clear();
    g_plume_backend.logged_renderdoc_pass_watch = false;
  } else if (g_plume_backend.last_renderdoc_pass_capture !=
             renderdoc_capture_pass) {
    if (!g_plume_backend.logged_renderdoc_pass_watch) {
      REXLOG_INFO("Refii plume RenderDoc pass watch active pass={}",
                  renderdoc_capture_pass);
      g_plume_backend.logged_renderdoc_pass_watch = true;
    }
    constexpr size_t kMaxRenderDocPassCommandProbe = 4096;
    if (g_plume_backend.renderdoc_pass_capture_repeats != 0) {
      if (RequestRenderDocFrameCapture()) {
        --g_plume_backend.renderdoc_pass_capture_repeats;
        if (g_plume_backend.renderdoc_pass_capture_repeats == 0) {
          g_plume_backend.last_renderdoc_pass_capture = renderdoc_capture_pass;
        }
        REXLOG_INFO(
            "Refii plume RenderDoc pass capture repeat pass={} present={} "
            "remaining={}",
            renderdoc_capture_pass, renderdoc_capture_frame_index,
            g_plume_backend.renderdoc_pass_capture_repeats);
      }
    } else if (FableDirectQueuedRenderPassPresent(
                   renderdoc_capture_pass, kMaxRenderDocPassCommandProbe)) {
      if (RequestRenderDocFrameCapture()) {
        // Capture 3 consecutive frames; the queued-command probe runs one
        // frame out of phase with execution.
        g_plume_backend.renderdoc_pass_capture_repeats = 2;
        REXLOG_INFO(
            "Refii plume RenderDoc pass capture armed pass={} present={}",
            renderdoc_capture_pass, renderdoc_capture_frame_index);
      }
    }
  }
  const PlumeRenderDocAutoCaptureArm renderdoc_auto_capture =
      MaybeArmPlumeRenderDocAutoContentCapture(
          renderdoc_capture_frame_index, renderdoc_ready_to_capture,
          renderdoc_capture_frame, renderdoc_capture_draw_index,
          renderdoc_capture_draw_target, renderdoc_capture_pass);
  const std::string_view renderdoc_effective_capture_draw_target =
      renderdoc_capture_draw_target.empty()
          ? std::string_view(renderdoc_auto_capture.draw_target)
          : std::string_view(renderdoc_capture_draw_target);
  const std::string renderdoc_capture_reason = RenderDocCaptureReason();
  const std::string renderdoc_capture_file_tag =
      BuildRenderDocCaptureFileTag(renderdoc_capture_frame_index,
                                   renderdoc_capture_draw_index,
                                   renderdoc_effective_capture_draw_target,
                                   renderdoc_capture_pass,
                                   renderdoc_capture_reason);
  const bool renderdoc_capture_file_tag_pending =
      RenderDocCaptureRequestPending() || RenderDocTriggerCaptureRequestPending();
  if (renderdoc_capture_file_tag_pending) {
    SetRenderDocFrameCaptureFileTag(renderdoc_capture_file_tag.c_str());
  }
  // Present-decoupling step 3: decide record vs re-present BEFORE the capture
  // begins or any fable frame state is touched. Record whenever anything could
  // change the composed output — a new guest boundary, drainable/carried GTL
  // packets (FableDirectFrameRecordReason), fresh guest-side deferred texel
  // captures or Bink uploads (generation compares against the last record) —
  // or when RenderDoc wants this present (a capture of a blit-only frame is
  // useless). The present itself is NEVER skipped (run-328 post-mortem): the
  // fast path replaces only the record with a blit of the last completed
  // compose target; guest-servicing work stays on every wake.
  const uint64_t present_bink_upload_count = PlumeBinkVideoUploadReadyCount();
  const uint64_t present_deferred_capture_generation =
      DeferredTextureCaptureEnqueueGeneration();
  PlumeComposeTarget* held_compose_target =
      g_plume_backend.last_recorded_compose_target <
              g_plume_backend.compose_targets.size()
          ? &g_plume_backend
                 .compose_targets[g_plume_backend.last_recorded_compose_target]
          : nullptr;
  const bool renderdoc_wants_present =
      renderdoc_capture_file_tag_pending || RenderDocFrameCaptureActive();
  // First matching reason wins; kPlumeRecordWhyCount means none — fast path.
  size_t record_why = kPlumeRecordWhyCount;
  if (held_compose_target == nullptr || !held_compose_target->recorded ||
      held_compose_target->color_texture == nullptr) {
    record_why = kPlumeRecordWhyNoCache;
  } else if (renderdoc_wants_present) {
    record_why = kPlumeRecordWhyRenderDoc;
  } else if (present_bink_upload_count !=
             g_plume_backend.last_record_bink_upload_count) {
    record_why = kPlumeRecordWhyBink;
  } else {
    if (present_deferred_capture_generation !=
        g_plume_backend.last_record_deferred_capture_generation) {
      // Captures ONLY re-record after a minimum interval (run 461: the guest
      // enqueues captures spread across its whole frame, so a ~100ms record
      // always overlapped a fresh enqueue and captures-caused records ran
      // back-to-back — zero blit windows, presents capped at 1/record-time).
      // This bounds capture staleness at ~one guest frame, the already-
      // accepted risk. Boundary/bink/video reasons stay immediate below —
      // real new frames never wait.
      constexpr std::chrono::milliseconds kPlumeCapturesRerecordMinInterval{
          100};
      if (g_plume_backend.last_record_completed_at ==
              PlumeClock::time_point{} ||
          PlumeClock::now() - g_plume_backend.last_record_completed_at >
              kPlumeCapturesRerecordMinInterval) {
        record_why = kPlumeRecordWhyCaptures;
      } else {
        g_plume_present_census_captures_deferred.fetch_add(
            1, std::memory_order_relaxed);
      }
    }
    if (record_why == kPlumeRecordWhyCount) {
      switch (FableDirectFrameRecordReason()) {
        case FableDirectRecordReason::kNone:
          break;
        case FableDirectRecordReason::kNotAuthoritative:
          record_why = kPlumeRecordWhyMode;
          break;
        case FableDirectRecordReason::kStartupVideoPump:
          record_why = kPlumeRecordWhyVideo;
          break;
        case FableDirectRecordReason::kGuestBoundary:
          record_why = kPlumeRecordWhyBoundary;
          break;
        case FableDirectRecordReason::kCarriedPackets:
          record_why = kPlumeRecordWhyCarried;
          break;
        case FableDirectRecordReason::kPendingPackets:
          record_why = kPlumeRecordWhyPackets;
          break;
      }
    }
  }
  const bool skip_record = record_why == kPlumeRecordWhyCount;
  if (renderdoc_ready_to_capture && ConsumeRenderDocTriggerCaptureRequest()) {
    TriggerRenderDocCapture();
    REXLOG_INFO(
        "Refii plume RenderDoc-owned capture trigger consumed present={} "
        "tag={}",
        renderdoc_capture_frame_index, renderdoc_capture_file_tag);
  }
  const bool renderdoc_capture_started =
      BeginRenderDocFrameCapture("Refii Plume Frame",
                                 renderdoc_ready_to_capture);
  auto end_renderdoc_started_capture = [&]() {
    EndRenderDocFrameCapture(renderdoc_capture_started);
    if (renderdoc_capture_started) {
      ClearRenderDocCaptureReason();
    }
  };

  if (g_plume_backend.frame_contexts.empty()) {
    end_renderdoc_started_capture();
    return false;
  }
  const uint32_t frame_context_index =
      g_plume_backend.next_frame_context %
      static_cast<uint32_t>(g_plume_backend.frame_contexts.size());
  PlumeFrameContext& frame_context =
      g_plume_backend.frame_contexts[frame_context_index];
  g_plume_backend.next_frame_context =
      (frame_context_index + 1) %
      static_cast<uint32_t>(g_plume_backend.frame_contexts.size());
  const PlumeClock::time_point frame_context_wait_t0 = PlumeClock::now();
  {
    ZoneScopedN("RefiiPlume/WaitReusableFrameContext");
    WaitPlumeFrameContextLocked(frame_context);
  }
  // Step 2 compose-target rotation. On the record path, take the next
  // alternating offscreen target and wait out the frame context that last
  // used it: the direct queue is in-order so a later fence covers earlier
  // submissions, and this is what lets a re-present blit of target A overlap
  // a later record into target B without ever racing A's read.
  PlumeComposeTarget* compose_record_target = nullptr;
  if (!skip_record) {
    const uint32_t compose_index =
        g_plume_backend.next_compose_target %
        static_cast<uint32_t>(g_plume_backend.compose_targets.size());
    compose_record_target = &g_plume_backend.compose_targets[compose_index];
    if (compose_record_target->last_use_frame_context <
        g_plume_backend.frame_contexts.size()) {
      WaitPlumeFrameContextLocked(
          g_plume_backend
              .frame_contexts[compose_record_target->last_use_frame_context]);
    }
    if (!compose_record_target->color_texture ||
        !compose_record_target->framebuffer ||
        !compose_record_target->depth_framebuffer) {
      end_renderdoc_started_capture();
      return false;
    }
  }
  const PlumeClock::time_point frame_context_wait_t1 = PlumeClock::now();

  uint32_t image_index = 0;
  {
    ZoneScopedN("RefiiPlume/AcquireTexture");
    if (!g_plume_backend.swap_chain->acquireTexture(
            frame_context.acquire_semaphore.get(), &image_index)) {
      end_renderdoc_started_capture();
      return false;
    }
  }
  const PlumeClock::time_point acquire_t1 = PlumeClock::now();

  if (image_index >= g_plume_backend.framebuffers.size()) {
    end_renderdoc_started_capture();
    return false;
  }
  if (g_plume_backend.depth_texture &&
      image_index >= g_plume_backend.depth_framebuffers.size()) {
    end_renderdoc_started_capture();
    return false;
  }
  // Step 2: the fable frame records into the offscreen compose target, never
  // the acquired swapchain image; the swapchain image receives the compose by
  // blit below, then the diagnostics/ImGui overlay exactly as before. Null on
  // the step-3 fast path, which records no fable frame at all.
  plume::RenderFramebuffer* fable_output_framebuffer = nullptr;
  if (compose_record_target) {
    fable_output_framebuffer =
        g_plume_backend.depth_texture
            ? compose_record_target->depth_framebuffer.get()
            : compose_record_target->framebuffer.get();
  }

  plume::RenderTexture* swap_chain_texture =
      g_plume_backend.swap_chain->getTexture(image_index);
  plume::RenderCommandList* command_list = frame_context.command_list.get();
  if (!command_list) {
    end_renderdoc_started_capture();
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
    PlumeDebugLabelScope imgui_upload_scope(command_list,
                                            "RefiiPlume::ImGuiUploads");
    RecordPlumeImGuiUploads(command_list);
  }
  if (!g_plume_backend.logged_frame_after_uploads) {
    REXLOG_INFO("Refii plume frame recorded pending uploads");
    g_plume_backend.logged_frame_after_uploads = true;
  }
  if (compose_record_target) {
    command_list->barriers(
        plume::RenderBarrierStage::GRAPHICS,
        plume::RenderTextureBarrier(compose_record_target->color_texture.get(),
                                    plume::RenderTextureLayout::COLOR_WRITE));
    if (g_plume_backend.depth_texture) {
      command_list->barriers(
          plume::RenderBarrierStage::GRAPHICS,
          plume::RenderTextureBarrier(g_plume_backend.depth_texture.get(),
                                      plume::RenderTextureLayout::DEPTH_WRITE));
    }
    command_list->setFramebuffer(fable_output_framebuffer);
    if (!g_plume_backend.logged_frame_after_color_target) {
      REXLOG_INFO("Refii plume frame attached offscreen compose color target");
      g_plume_backend.logged_frame_after_color_target = true;
    }
  }

  const uint32_t width = g_plume_backend.swap_chain->getWidth();
  const uint32_t height = g_plume_backend.swap_chain->getHeight();
  const uint32_t frame_index = ++g_plume_backend.present_count;
  PlumeDebugLabelScope frame_debug_label(
      command_list, BuildFrameDebugScopeLabel(frame_index, width, height));
  g_plume_output_present_count.store(frame_index, std::memory_order_relaxed);
  const PlumeClock::time_point setup_t1 = PlumeClock::now();
  if (compose_record_target) {
    BeginFableDirectFrame(frame_index);
    direct_snapshot = GetFableDirectStateSnapshot();
  }
  const bool log_frame_summary =
      compose_record_target != nullptr &&
      (!g_plume_backend.logged_first_frame_summary ||
       (RendererVerboseTracingEnabled() && frame_index % 120 == 0));
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
        "title_lists={}/{}/{} nodes={} last_title=0x{:08X} "
        "object=0x{:08X}/vt=0x{:08X} node=0x{:08X}/vt=0x{:08X} "
        "title_world={}/0x{:08X} registry=0x{:08X} count={} matched=0x{:08X}/{} "
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
        hook_snapshot.shader_constant_rejected_uploads,
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
        hook_snapshot.title_render_list_dispatches,
        hook_snapshot.title_render_list_insert_callbacks,
        hook_snapshot.title_render_list_sorted_inserts,
        hook_snapshot.title_render_list_inserted_nodes,
        hook_snapshot.last_title_render_list,
        hook_snapshot.last_title_render_object,
        hook_snapshot.last_title_render_object_vtable,
        hook_snapshot.last_title_render_node,
        hook_snapshot.last_title_render_node_vtable,
        hook_snapshot.title_world_registry_observations,
        hook_snapshot.last_title_world,
        hook_snapshot.last_title_world_registry,
        hook_snapshot.last_title_world_registry_count,
        hook_snapshot.last_title_world_registry_matched_entry,
        hook_snapshot.last_title_world_registry_matched_object_count,
        PlumeFableCommandSourceName(command_source),
        PlumeFableCommandSourceName(command_source),
        direct_snapshot.render_command_count,
        direct_snapshot.submitted_render_command_count,
        direct_snapshot.submitted_render_command_serial,
        direct_snapshot.command_boundary_capture_count,
        direct_snapshot.empty_command_boundary_capture_count,
        direct_snapshot.last_command_boundary_submit,
        direct_snapshot.last_command_boundary_staged_commands,
        direct_snapshot.last_command_boundary_staged_draws,
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
  if (compose_record_target && !preserve_direct_output) {
    PlumeDebugLabelScope clear_scope(command_list,
                                     "RefiiPlume::ClearOutput");
    command_list->clearColor(0, plume::RenderColor(0.0f, 0.0f, 0.0f, 1.0f));
    if (g_plume_backend.depth_texture) {
      command_list->clearDepthStencil(true, false, 1.0f, 0);
    }
  } else if (compose_record_target &&
             !g_plume_backend.logged_preserved_fable_direct_output) {
    REXLOG_INFO(
        "Refii plume preserving Fable direct output instead of pre-clearing "
        "the compose target draw_count={} commands={} submitted={} serial={}",
        direct_snapshot.draw_count, direct_snapshot.render_command_count,
        direct_snapshot.submitted_render_command_count,
        direct_snapshot.submitted_render_command_serial);
    g_plume_backend.logged_preserved_fable_direct_output = true;
  }
  FableDirectRendererStats direct_stats = {};
  if (compose_record_target) {
    direct_stats = ExecuteRefiiRenderGraphFrame(
        g_plume_backend.device.get(), command_list, fable_output_framebuffer,
        compose_record_target->color_texture.get(),
        compose_record_target->framebuffer.get(), shader_format,
        g_plume_backend.depth_texture ? plume::RenderFormat::D32_FLOAT_S8_UINT
                                      : plume::RenderFormat::UNKNOWN,
        frame_index, width, height);
    if (direct_stats.draw_count != 0 ||
        direct_stats.last_present_target != 0) {
      g_plume_backend.has_fable_direct_output_to_preserve = true;
    }
  }
  const bool renderdoc_owned_capture_active =
      renderdoc_capture_started || direct_stats.renderdoc_capture_started;
  const bool renderdoc_observed_capture_active = RenderDocFrameCaptureActive();
  const bool renderdoc_capture_active =
      renderdoc_owned_capture_active || renderdoc_observed_capture_active;
  if (renderdoc_observed_capture_active && !renderdoc_owned_capture_active) {
    REXLOG_INFO(
        "Refii plume observed external RenderDoc frame capture; writing frame "
        "database and hook trace sidecars frame={}",
        frame_index);
    if (direct_stats.draw_count == 0 && direct_stats.last_draw_events == 0 &&
        direct_stats.last_render_commands == 0 &&
        direct_stats.last_present_target != 0) {
      REXLOG_WARN(
          "Refii plume external RenderDoc capture is present-only; use a "
          "Refii-owned target/pass capture for title/Bink analysis frame={} "
          "present=0x{:08X} size={}x{}",
          frame_index, direct_stats.last_present_target,
          direct_stats.last_present_width, direct_stats.last_present_height);
    }
  }
  const bool renderdoc_record_time_target =
      IsRenderDocResolveCaptureTarget(renderdoc_effective_capture_draw_target) ||
      IsRenderDocSurfaceDestinationCaptureTarget(
          renderdoc_effective_capture_draw_target);
  const std::string_view renderdoc_sidecar_draw_target =
      renderdoc_record_time_target && !direct_stats.renderdoc_capture_started
          ? std::string_view()
          : renderdoc_effective_capture_draw_target;
  const PlumeClock::time_point direct_t1 = PlumeClock::now();
  const RendererHookObserverSnapshot frame_hook_snapshot =
      GetRendererHookObserverSnapshot();
  const std::string frame_correlation_label = BuildFrameCorrelationLabel(
      frame_index, width, height, direct_stats, frame_hook_snapshot);
  command_list->insertDebugLabel(frame_correlation_label);
  ZoneTextV(tracy_frame_zone, frame_correlation_label.c_str(),
            frame_correlation_label.size());
  if (renderdoc_capture_active) {
    const RendererHookTimingSnapshot renderdoc_timing_snapshot =
        GetRendererHookTimingSnapshot();
    const std::string frame_database_path = WriteRenderDocFrameDatabaseDump(
        frame_index, width, height, direct_stats, frame_hook_snapshot,
        direct_snapshot, frame_correlation_label, renderdoc_capture_draw_index,
        renderdoc_sidecar_draw_target, renderdoc_capture_pass,
        renderdoc_capture_reason, renderdoc_owned_capture_active,
        direct_stats.renderdoc_capture_started);
    const std::string hook_trace_path = WriteRenderDocHookTraceDump(
        frame_index, width, height, frame_correlation_label,
        renderdoc_capture_draw_index, renderdoc_sidecar_draw_target,
        renderdoc_capture_pass, renderdoc_capture_reason,
        renderdoc_owned_capture_active, direct_stats.renderdoc_capture_started);
    SetRenderDocFrameCaptureTitle(BuildRenderDocCaptureTitle(
                                      frame_correlation_label)
                                      .c_str());
    SetRenderDocFrameCaptureComments(
        BuildRenderDocCaptureComments(frame_index, width, height, direct_stats,
                                      frame_hook_snapshot,
                                      renderdoc_timing_snapshot,
                                      direct_snapshot, frame_database_path,
                                      hook_trace_path, frame_correlation_label,
                                      renderdoc_capture_draw_index,
                                      renderdoc_sidecar_draw_target,
                                      renderdoc_capture_pass,
                                      renderdoc_capture_reason)
            .c_str());
  }
  // Step 2 blit: offscreen compose -> acquired swapchain image. On the step-3
  // fast path the source is the last completed record; otherwise the target
  // recorded above. The ImGui dimmer + overlay and diagnostics then draw into
  // the swapchain image on top, exactly as they previously drew after the
  // scene.
  PlumeComposeTarget* compose_blit_source =
      compose_record_target ? compose_record_target : held_compose_target;
  {
    ZoneScopedN("RefiiPlume/ComposeBlit");
    PlumeDebugLabelScope blit_scope(command_list, "RefiiPlume::ComposeBlit");
    command_list->barriers(
        plume::RenderBarrierStage::COPY,
        plume::RenderTextureBarrier(compose_blit_source->color_texture.get(),
                                    plume::RenderTextureLayout::COPY_SOURCE));
    command_list->barriers(
        plume::RenderBarrierStage::COPY,
        plume::RenderTextureBarrier(swap_chain_texture,
                                    plume::RenderTextureLayout::COPY_DEST));
    command_list->copyTexture(swap_chain_texture,
                              compose_blit_source->color_texture.get());
    command_list->barriers(
        plume::RenderBarrierStage::GRAPHICS,
        plume::RenderTextureBarrier(swap_chain_texture,
                                    plume::RenderTextureLayout::COLOR_WRITE));
  }
  command_list->setFramebuffer(g_plume_backend.framebuffers[image_index].get());
  {
    ZoneScopedN("RefiiPlume/RenderDiagnostics");
    PlumeDebugLabelScope diagnostics_scope(command_list,
                                           "RefiiPlume::Diagnostics");
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
    PlumeDebugLabelScope imgui_scope(command_list,
                                     "RefiiPlume::ImGuiOverlay");
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
  frame_debug_label.End();
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
    EndRenderDocFrameCapture(renderdoc_owned_capture_active);
    if (renderdoc_capture_started) {
      ClearRenderDocCaptureReason();
    }
    return false;
  }
  // Step 2/3 bookkeeping: whichever compose target this frame used (recorded
  // into or blitted from) is now covered by this frame context's fence.
  if (compose_record_target) {
    compose_record_target->recorded = true;
    compose_record_target->last_use_frame_context = frame_context_index;
    g_plume_backend.last_recorded_compose_target = static_cast<uint32_t>(
        compose_record_target - g_plume_backend.compose_targets.data());
    g_plume_backend.next_compose_target =
        (g_plume_backend.last_recorded_compose_target + 1u) %
        static_cast<uint32_t>(g_plume_backend.compose_targets.size());
    g_plume_backend.last_record_present_target =
        direct_stats.last_present_target;
    g_plume_backend.last_record_bink_upload_count = present_bink_upload_count;
    // Stamp the capture generation POST-record (not the pre-record snapshot,
    // run 461): the record's own drain consumed everything queued before it,
    // and an enqueue that landed mid-record is absorbed here — it is at most
    // one captures-interval stale, not a reason for a back-to-back record.
    g_plume_backend.last_record_deferred_capture_generation =
        DeferredTextureCaptureEnqueueGeneration();
    g_plume_backend.last_record_completed_at = PlumeClock::now();
    g_plume_present_census_rerecords.fetch_add(1, std::memory_order_relaxed);
    if (record_why < kPlumeRecordWhyCount) {
      g_plume_present_record_why[record_why].fetch_add(
          1, std::memory_order_relaxed);
    }
  } else {
    compose_blit_source->last_use_frame_context = frame_context_index;
    g_plume_present_census_represents.fetch_add(1, std::memory_order_relaxed);
  }
  bool presented = false;
  {
    ZoneScopedN("RefiiPlume/SwapChainPresent");
    presented = g_plume_backend.swap_chain->present(
        image_index, &signal_semaphore, 1);
    if (presented) {
      g_plume_present_census_presented.fetch_add(1,
                                                 std::memory_order_relaxed);
    }
    if (presented && g_plume_backend.swap_chain_present_wait_enabled) {
      g_plume_backend.swap_chain->wait();
    }
  }
  const PlumeClock::time_point present_t1 = PlumeClock::now();
  // Fable display bookkeeping runs on EVERY present (run-328 post-mortem: the
  // guest's swap acknowledgement must never be gated on the record path); the
  // fast path re-states the last record's present target.
  PresentFableDirectFrame(compose_record_target
                              ? direct_stats.last_present_target
                              : g_plume_backend.last_record_present_target,
                          width, height);
  if (compose_record_target) {
    EndFableDirectFrame();
  }
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
  {
    // Step 1 census: time-limited to ~2s so every phase (menu 1-2ms composes
    // through gameplay ~100ms composes) gets a steady cadence. Cumulative
    // counters — read deltas between lines, not absolutes.
    static PlumeClock::time_point s_last_present_census_log{};
    const PlumeClock::time_point census_now = PlumeClock::now();
    if (s_last_present_census_log == PlumeClock::time_point{} ||
        census_now - s_last_present_census_log >= std::chrono::seconds(2)) {
      s_last_present_census_log = census_now;
      // STEP 6 gate census (cumulative, like every counter on this line):
      // checks = all gate evaluations, disarmed = ones skipped by the
      // publish-bootstrap/startup-video guards (armed = checks - disarmed;
      // run-513: counting checks after the guards made a video-phase segment
      // read as "gate never executed"), waits = ones that blocked, wait_ms =
      // total blocked guest-thread time, timeouts = bounded-wait expiries
      // (gate degraded to advisory), outstanding = present-captured boundary
      // lag at gate entry (max/last; 0-1 = depth held for free), boundary =
      // present_seq/captured_seq, publishes = record completions.
      const FableCaptureAckGateCensus ack_census =
          GetFableCaptureAckGateCensus();
      REXLOG_INFO(
          "Refii present census requests={} coalesced_dropped={} presented={} "
          "rerecords={} represents={} record_why[nocache={} renderdoc={} "
          "bink={} captures={} mode={} video={} boundary={} carried={} "
          "packets={}] captures_deferred={} capture_ack[checks={} disarmed={} "
          "waits={} wait_ms={} timeouts={} outstanding_max={} "
          "outstanding_last={} boundary={}/{} publishes={}]",
          g_plume_present_census_requests.load(std::memory_order_relaxed),
          g_plume_present_census_coalesced_dropped.load(
              std::memory_order_relaxed),
          g_plume_present_census_presented.load(std::memory_order_relaxed),
          g_plume_present_census_rerecords.load(std::memory_order_relaxed),
          g_plume_present_census_represents.load(std::memory_order_relaxed),
          g_plume_present_record_why[kPlumeRecordWhyNoCache].load(
              std::memory_order_relaxed),
          g_plume_present_record_why[kPlumeRecordWhyRenderDoc].load(
              std::memory_order_relaxed),
          g_plume_present_record_why[kPlumeRecordWhyBink].load(
              std::memory_order_relaxed),
          g_plume_present_record_why[kPlumeRecordWhyCaptures].load(
              std::memory_order_relaxed),
          g_plume_present_record_why[kPlumeRecordWhyMode].load(
              std::memory_order_relaxed),
          g_plume_present_record_why[kPlumeRecordWhyVideo].load(
              std::memory_order_relaxed),
          g_plume_present_record_why[kPlumeRecordWhyBoundary].load(
              std::memory_order_relaxed),
          g_plume_present_record_why[kPlumeRecordWhyCarried].load(
              std::memory_order_relaxed),
          g_plume_present_record_why[kPlumeRecordWhyPackets].load(
              std::memory_order_relaxed),
          g_plume_present_census_captures_deferred.load(
              std::memory_order_relaxed),
          ack_census.checks, ack_census.disarms, ack_census.waits,
          ack_census.wait_us / 1000u, ack_census.timeouts,
          ack_census.max_outstanding, ack_census.last_outstanding,
          ack_census.present_boundary, ack_census.captured_boundary,
          ack_census.publishes);

      // GUEST RENDER STALL WATCHDOG (2026-08-02, save-load black screen).
      // Run 516/517 signature: the guest render thread executes a burst of
      // `twi 31,r0,22` (__debugbreak) and never issues another hooked D3D
      // call, so `rerecords` freezes while `represents` keeps climbing — the
      // pump re-presents one stale frame forever and the screen reads black.
      // `presented` keeps advancing in that state, so it is NOT a liveness
      // signal; rerecords (new guest content recorded) is the only one.
      //
      // On the transition into that state, emit exactly ONE line naming every
      // guest thread's last hooked symbol and how long ago it ran. The thread
      // whose age jumps to seconds while the others keep ticking is the one
      // that died, and its symbol is the last guest function we saw it in —
      // which is what pins the trap site. One-shot per stall episode; re-arms
      // if content publication resumes.
      static uint64_t s_watchdog_last_rerecords = 0u;
      static uint32_t s_watchdog_stalled_censuses = 0u;
      static bool s_watchdog_reported = false;
      const uint64_t watchdog_rerecords =
          g_plume_present_census_rerecords.load(std::memory_order_relaxed);
      if (watchdog_rerecords != s_watchdog_last_rerecords) {
        s_watchdog_last_rerecords = watchdog_rerecords;
        s_watchdog_stalled_censuses = 0u;
        s_watchdog_reported = false;
      } else if (watchdog_rerecords != 0u) {
        // Census cadence is ~2s, so 3 consecutive stalls is ~6s of zero new
        // guest content — far outside any legitimate compose or load hitch.
        ++s_watchdog_stalled_censuses;
        if (s_watchdog_stalled_censuses >= 3u && !s_watchdog_reported) {
          s_watchdog_reported = true;
          refii::renderer::RendererHookThreadTelemetryEntry threads[64];
          const size_t thread_count =
              refii::renderer::SnapshotRendererHookThreadTelemetry(
                  threads, std::size(threads));
          std::string autopsy;
          for (size_t i = 0; i < thread_count; ++i) {
            if (!autopsy.empty()) {
              autopsy += " | ";
            }
            autopsy += fmt::format("tid=0x{:08X} last_hook={} age_us={}",
                                   threads[i].guest_tid,
                                   threads[i].last_symbol != nullptr
                                       ? threads[i].last_symbol
                                       : "none",
                                   threads[i].us_since_enter);
          }
          REXLOG_WARN(
              "Refii GUEST RENDER STALLED: no new guest content for ~{}s "
              "(rerecords frozen at {}, represents={} presented={} still "
              "climbing = pump re-presenting a stale frame). Guest thread "
              "autopsy [{} threads]: {} -- the thread whose age_us is large "
              "while others tick is the stalled one; its last_hook is the "
              "last guest function it entered.",
              s_watchdog_stalled_censuses * 2u, watchdog_rerecords,
              g_plume_present_census_represents.load(std::memory_order_relaxed),
              g_plume_present_census_presented.load(std::memory_order_relaxed),
              thread_count, autopsy.empty() ? "(registry empty)" : autopsy);
        }
      }
    }
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
  StorePlumeFrameHistoryEntry(frame_index, width, height, presented,
                              renderdoc_capture_active, direct_stats,
                              frame_total_us, direct_us, submit_us,
                              present_us);
  if (renderdoc_capture_active) {
    FableDirectRendererStats post_present_direct_stats = direct_stats;
    post_present_direct_stats.presented = presented;
    post_present_direct_stats.last_present_width = width;
    post_present_direct_stats.last_present_height = height;
    WriteRenderDocFrameDatabaseDump(
        frame_index, width, height, post_present_direct_stats,
        frame_hook_snapshot, direct_snapshot, frame_correlation_label,
        renderdoc_capture_draw_index, renderdoc_sidecar_draw_target,
        renderdoc_capture_pass, renderdoc_capture_reason,
        renderdoc_owned_capture_active, direct_stats.renderdoc_capture_started);
    WriteRenderDocHookTraceDump(
        frame_index, width, height, frame_correlation_label,
        renderdoc_capture_draw_index, renderdoc_sidecar_draw_target,
        renderdoc_capture_pass, renderdoc_capture_reason,
        renderdoc_owned_capture_active, direct_stats.renderdoc_capture_started);
  }
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
  const RendererShaderLibrarySnapshot tracy_shader_snapshot =
      GetRendererShaderLibrarySnapshot();
  const int64_t tracy_pipeline_creates =
      static_cast<int64_t>(direct_stats.pipeline_cache_misses);
  const int64_t tracy_shader_misses =
      static_cast<int64_t>(tracy_shader_snapshot.missing_binary_count);
  const int64_t tracy_missing_textures =
      static_cast<int64_t>(direct_stats.failed_resource_syncs);
  const RefiiFramePacketQueueSnapshot tracy_packet_queue =
      GetRefiiFramePacketQueueSnapshot();
  TracyPlot("Refii.DrawPackets", static_cast<int64_t>(direct_stats.draw_count));
  TracyPlot("Refii.PacketQueueDepth",
            static_cast<int64_t>(tracy_packet_queue.packets.size()));
  TracyPlot("Refii.PacketActionQueueDepth",
            static_cast<int64_t>(tracy_packet_queue.actions.size()));
  TracyPlot("Refii.PacketReplayPlannedActions",
            static_cast<int64_t>(
                tracy_packet_queue.replay.planned_action_packets));
  TracyPlot("Refii.PacketReplayPlannedCopies",
            static_cast<int64_t>(
                tracy_packet_queue.replay.planned_copy_packets));
  TracyPlot("Refii.PacketReplayPlannedPresents",
            static_cast<int64_t>(
                tracy_packet_queue.replay.planned_present_packets));
  TracyPlot("Refii.PacketReplayPlanned",
            static_cast<int64_t>(
                tracy_packet_queue.replay.planned_draw_packets));
  TracyPlot("Refii.PacketReplayUnsupported",
            static_cast<int64_t>(
                tracy_packet_queue.replay.unsupported_packets));
  TracyPlot("Refii.PipelineCreates", tracy_pipeline_creates);
  TracyPlot("Refii.PipelineCacheMisses",
            static_cast<int64_t>(direct_stats.pipeline_cache_misses));
  TracyPlot("Refii.ShaderMisses", tracy_shader_misses);
  TracyPlot("Refii.TextureUploads",
            static_cast<int64_t>(direct_stats.resource_uploads));
  TracyPlot("Refii.BufferUploads", int64_t{0});
  TracyPlot("Refii.MissingResources",
            static_cast<int64_t>(direct_stats.failed_resource_syncs));
  TracyPlot("Refii.MissingTextures", tracy_missing_textures);
  TracyPlot("Refii.MissingShaders", tracy_shader_misses);
  TracyPlot("Refii.RenderTargetSwitches",
            static_cast<int64_t>(direct_stats.render_target_switches));
  TracyPlot("Refii.DepthTargetSwitches",
            static_cast<int64_t>(direct_stats.depth_target_switches));
  TracyPlot("Refii.FrameDatabaseBytes",
            static_cast<int64_t>(FrameDatabasePacketBytesEstimate(direct_stats)));
  const FableDirectFrameDatabaseDrawPackets tracy_draw_packets =
      GetFableDirectFrameDatabaseDrawPackets();
  const FableDirectFrameDatabaseCommandPackets tracy_command_packets =
      GetFableDirectFrameDatabaseCommandPackets();
  const FrameDatabaseResourceNameIndex tracy_resource_name_index =
      BuildFrameDatabaseResourceNameIndex(GetFableDirectResourceDebugSnapshot());
  const std::vector<FrameDatabaseResourceEdge> tracy_resource_edges =
      BuildFrameDatabaseResourceEdges(tracy_command_packets);
  const FrameDatabaseResourceEdgeCounts tracy_resource_edge_counts =
      CountFrameDatabaseResourceEdges(tracy_resource_edges);
  TracyPlot("Refii.CommandPackets",
            static_cast<int64_t>(
                CountFrameDatabaseExecutableCommandPackets(
                    tracy_command_packets)));
  TracyPlot("Refii.ClearPackets",
            static_cast<int64_t>(
                CountFrameDatabaseCommandKind(tracy_command_packets, "clear")));
  TracyPlot(
      "Refii.ResolvePackets",
      static_cast<int64_t>(
          CountFrameDatabaseCommandKind(tracy_command_packets, "resolve")));
  TracyPlot("Refii.PresentBlitPackets",
            static_cast<int64_t>(CountFrameDatabaseCommandKind(
                tracy_command_packets, "present_blit")));
  TracyPlot("Refii.ResourceEdges",
            static_cast<int64_t>(tracy_resource_edge_counts.total_edges));
  TracyPlot("Refii.ClearResourceEdges",
            static_cast<int64_t>(tracy_resource_edge_counts.clear_edges));
  TracyPlot("Refii.ResolveResourceEdges",
            static_cast<int64_t>(tracy_resource_edge_counts.resolve_edges));
  TracyPlot(
      "Refii.SurfaceDestinationEdges",
      static_cast<int64_t>(
          tracy_resource_edge_counts.surface_destination_edges));
  TracyPlot("Refii.PresentBlitEdges",
            static_cast<int64_t>(
                tracy_resource_edge_counts.present_blit_edges));
  const std::vector<FrameDatabasePassResourceUsage> tracy_pass_resource_usages =
      BuildFrameDatabasePassResourceUsages(tracy_draw_packets,
                                           tracy_resource_edges);
  TracyPlot("Refii.PassResourceUsages",
            static_cast<int64_t>(tracy_pass_resource_usages.size()));
  const std::vector<FrameDatabasePassSummary> tracy_pass_summaries =
      BuildFrameDatabasePassSummaries(tracy_draw_packets,
                                      tracy_command_packets,
                                      tracy_resource_edges);
  TracyPlot("Refii.PassClassified",
            static_cast<int64_t>(
                CountFrameDatabaseClassifiedPassSummaries(
                    tracy_pass_summaries)));
  const std::vector<FrameDatabaseFrameResourceLifetime>
      tracy_frame_resource_lifetimes =
          BuildFrameDatabaseFrameResourceLifetimes(tracy_pass_summaries,
                                                  tracy_pass_resource_usages);
  TracyPlot("Refii.FrameResourceLifetimes",
            static_cast<int64_t>(tracy_frame_resource_lifetimes.size()));
  const std::vector<FrameDatabaseMaterialRecord> tracy_material_records =
      BuildFrameDatabaseMaterialRecords(tracy_draw_packets,
                                        tracy_resource_name_index);
  TracyPlot("Refii.MaterialRecords",
            static_cast<int64_t>(tracy_material_records.size()));
  const std::vector<FrameDatabaseMeshRecord> tracy_mesh_records =
      BuildFrameDatabaseMeshRecords(tracy_draw_packets,
                                    tracy_resource_name_index);
  TracyPlot("Refii.MeshRecords",
            static_cast<int64_t>(tracy_mesh_records.size()));
  const std::vector<FrameDatabaseAssetUsageRecord> tracy_asset_usage_records =
      BuildFrameDatabaseAssetUsageRecords(tracy_draw_packets,
                                          tracy_resource_name_index);
  TracyPlot("Refii.AssetUsages",
            static_cast<int64_t>(tracy_asset_usage_records.size()));
  const std::vector<FrameDatabasePassDependency> tracy_pass_dependencies =
      BuildFrameDatabasePassDependencies(tracy_pass_summaries,
                                         tracy_pass_resource_usages);
  TracyPlot("Refii.PassDependencies",
            static_cast<int64_t>(tracy_pass_dependencies.size()));
  const std::vector<FrameDatabasePassResourceTransition>
      tracy_pass_resource_transitions =
          BuildFrameDatabasePassResourceTransitions(tracy_pass_dependencies);
  TracyPlot("Refii.ResourceTransitions",
            static_cast<int64_t>(tracy_pass_resource_transitions.size()));
  const std::vector<FrameDatabaseRenderGraphNode> tracy_render_graph_nodes =
      BuildFrameDatabaseRenderGraphNodes(tracy_pass_summaries,
                                        tracy_pass_resource_usages,
                                        tracy_asset_usage_records);
  TracyPlot("Refii.RenderGraphNodes",
            static_cast<int64_t>(tracy_render_graph_nodes.size()));
  const std::vector<FrameDatabaseRenderGraphEdge> tracy_render_graph_edges =
      BuildFrameDatabaseRenderGraphEdges(tracy_pass_dependencies);
  TracyPlot("Refii.RenderGraphEdges",
            static_cast<int64_t>(tracy_render_graph_edges.size()));
  const std::vector<FrameDatabaseRenderGraphExecutionStep>
      tracy_render_graph_execution_steps =
          BuildFrameDatabaseRenderGraphExecutionPlan(
              tracy_render_graph_nodes, tracy_render_graph_edges,
              tracy_pass_resource_transitions);
  TracyPlot("Refii.RenderGraphExecutionSteps",
            static_cast<int64_t>(tracy_render_graph_execution_steps.size()));
  TracyPlot("Refii.RenderGraphBarriers",
            static_cast<int64_t>(tracy_pass_resource_transitions.size()));
  const std::vector<FrameDatabaseRenderGraphPhase> tracy_render_graph_phases =
      BuildFrameDatabaseRenderGraphPhases(tracy_render_graph_nodes,
                                          tracy_render_graph_execution_steps);
  TracyPlot("Refii.RenderGraphPhases",
            static_cast<int64_t>(tracy_render_graph_phases.size()));
  TracyPlot("Refii.RenderGraphRuntimeExecutions", int64_t{1});
  TracyPlot("Refii.RenderGraphBridgeExecutions", int64_t{1});
  const RefiiRenderGraphRuntimeSchedulerSnapshot tracy_scheduler =
      GetRefiiRenderGraphRuntimeSchedulerSnapshot();
  TracyPlot("Refii.RenderGraphSchedulerExecutions",
            static_cast<int64_t>(tracy_scheduler.executions));
  TracyPlot("Refii.RenderGraphSchedulerSteps",
            static_cast<int64_t>(tracy_scheduler.steps));
  TracyPlot("Refii.RenderGraphSchedulerBridgeSteps",
            static_cast<int64_t>(tracy_scheduler.bridge_steps));
  TracyPlot("Refii.RenderGraphSchedulerGraphOwnedSteps",
            static_cast<int64_t>(tracy_scheduler.graph_owned_steps));
  TracyPlot("Refii.RenderGraphSchedulerGraphOwnedPlanSteps",
            static_cast<int64_t>(tracy_scheduler.graph_owned_plan_steps));
  TracyPlot("Refii.RenderGraphSchedulerGraphOwnedRenderSteps",
            static_cast<int64_t>(tracy_scheduler.graph_owned_render_steps));
  TracyPlot("Refii.RenderGraphSchedulerGraphOwnedClearSteps",
            static_cast<int64_t>(tracy_scheduler.graph_owned_clear_steps));
  TracyPlot("Refii.RenderGraphSchedulerGraphOwnedClearCommands",
            static_cast<int64_t>(tracy_scheduler.graph_owned_clear_commands));
  TracyPlot("Refii.RenderGraphSchedulerGraphOwnedDrawSteps",
            static_cast<int64_t>(tracy_scheduler.graph_owned_draw_steps));
  TracyPlot("Refii.RenderGraphSchedulerGraphOwnedDrawCommands",
            static_cast<int64_t>(tracy_scheduler.graph_owned_draw_commands));
  TracyPlot("Refii.RenderGraphSchedulerGraphOwnedResolveSteps",
            static_cast<int64_t>(tracy_scheduler.graph_owned_resolve_steps));
  TracyPlot("Refii.RenderGraphSchedulerGraphOwnedResolveCommands",
            static_cast<int64_t>(tracy_scheduler.graph_owned_resolve_commands));
  TracyPlot("Refii.RenderGraphSchedulerBridgeCommands",
            static_cast<int64_t>(tracy_scheduler.bridge_commands));
  const RefiiRenderGraphRuntimePlanSnapshot tracy_render_graph_runtime_plan =
      BuildRefiiRenderGraphRuntimePlanSnapshot(
          renderdoc_capture_frame_index, tracy_draw_packets,
          tracy_command_packets, tracy_pass_summaries, tracy_resource_edges,
          tracy_pass_resource_usages, tracy_render_graph_nodes,
          tracy_render_graph_execution_steps, tracy_render_graph_phases,
          tracy_pass_resource_transitions);
  StoreRefiiRenderGraphRuntimePlanSnapshot(tracy_render_graph_runtime_plan);
  TracyPlot("Refii.RenderGraphRuntimePlanPasses",
            static_cast<int64_t>(tracy_render_graph_runtime_plan.passes));
  TracyPlot(
      "Refii.RenderGraphRuntimePlanResources",
      static_cast<int64_t>(tracy_render_graph_runtime_plan.resource_references));
  TracyPlot("Refii.RenderGraphRuntimePlanEdgeOnlyPasses",
            static_cast<int64_t>(
                tracy_render_graph_runtime_plan.edge_only_passes));
  TracyPlot("Refii.RenderGraphRuntimePlanSurfaceDestinationEdges",
            static_cast<int64_t>(
                tracy_render_graph_runtime_plan.surface_destination_edges));
  TracyPlot("Refii.FrameHistoryEntries",
            static_cast<int64_t>(GetPlumeFrameHistorySnapshot().count));
  PlotFrameDatabasePassSummaries(tracy_pass_summaries);
  PlotFrameDatabaseRootCoverage(tracy_draw_packets);
  EndRenderDocFrameCapture(renderdoc_owned_capture_active);
  if (renderdoc_capture_started) {
    ClearRenderDocCaptureReason();
  }
  return presented;
#else
  return false;
#endif
}

void NotifyPlumeBackendHook(const char* symbol, RendererHookKind kind) {
  if (!PlumeRendererBackendEnabled()) {
    return;
  }
  // Once the one-shot log below has latched, this function has nothing left
  // to do — and it runs on EVERY hooked call (5,790/frame at title, run 415).
  // The unconditional try_lock was a per-call atomic RMW on the shared
  // backend mutex cacheline, ping-ponging against the render thread. Read
  // the latch first; the flag only ever goes false->true, so a stale read
  // costs one harmless extra try_lock.
  if (g_plume_backend.logged_hook_observer) {
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
    const RendererGraphicsApi api = GetRendererGraphicsApi();
    REXLOG_INFO(
        "Refii plume/{} hook observer is active; {} ({}) is feeding the "
        "static Plume replacement path while Plume owns presentation and "
        "routes captured Fable command submission through the replacement "
        "backend",
        PlumeApiBridgeName(api), symbol, hook_class);
    g_plume_backend.logged_hook_observer = true;
  }
}

}  // namespace refii::renderer
